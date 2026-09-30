import { render, screen, waitFor } from "@testing-library/react";
import { QueryClientProvider } from "@tanstack/react-query";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

/**
 * Settings -> Interface's default movie/show view used to have no effect:
 * useLibraryFilters read only localStorage, so a library this browser had
 * never opened before always started in the hardcoded grid (#736). The page
 * is what feeds the real saved default into LibraryView, now from the shared
 * route cache (#1081).
 */
const fetchJson = vi.hoisted(() => vi.fn());
vi.mock("../lib/api", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/api")>()),
  fetchJson
}));

vi.mock("../components/app/library-view", () => ({
  LibraryView: ({ variant, defaultView }: { variant: string; defaultView: string }) => (
    <div data-testid="library-view">{variant}:{defaultView}</div>
  )
}));

const { MoviesPage, ShowsPage, moviesLoader } = await import("./library-page");
const { queryClient } = await import("../lib/route-cache");

function answer(settings: unknown) {
  fetchJson.mockImplementation((path: string) => Promise.resolve(path === "/api/settings" ? settings : null));
}

function renderPage(page: "movies" | "shows") {
  const router = createMemoryRouter([{ path: "/", element: page === "movies" ? <MoviesPage /> : <ShowsPage /> }]);
  return render(
    <QueryClientProvider client={queryClient}>
      <RouterProvider router={router} />
    </QueryClientProvider>
  );
}

describe("library pages carry the saved default view (#736)", () => {
  beforeEach(() => queryClient.clear());
  afterEach(() => fetchJson.mockReset());

  it("passes the saved default movie view through to the movies page", async () => {
    answer({ defaultMovieView: "list" });
    renderPage("movies");
    expect(await screen.findByTestId("library-view")).toHaveTextContent("movies:list");
  });

  it("passes the saved default show view through to the shows page", async () => {
    answer({ defaultShowView: "list" });
    renderPage("shows");
    expect(await screen.findByTestId("library-view")).toHaveTextContent("shows:list");
  });

  it("falls back to grid for an unrecognised or missing saved default", async () => {
    answer({ defaultMovieView: "coverflow" });
    renderPage("movies");
    expect(await screen.findByTestId("library-view")).toHaveTextContent("movies:grid");
  });

  it("falls back to grid when the settings request itself failed", async () => {
    fetchJson.mockImplementation((path: string) => (path === "/api/settings" ? Promise.reject(new Error("down")) : Promise.resolve(null)));
    renderPage("movies");
    await waitFor(() => expect(screen.getByTestId("library-view")).toHaveTextContent("movies:grid"));
  });

  it("opens straight in the chosen layout on a revisit, before any answer comes back (#1081)", async () => {
    answer({ defaultMovieView: "list" });
    const first = renderPage("movies");
    await screen.findByTestId("library-view");
    first.unmount();

    // The host is now slow: nothing answers. The revisit still draws at once.
    fetchJson.mockImplementation(() => new Promise(() => {}));
    renderPage("movies");
    expect(screen.getByTestId("library-view")).toHaveTextContent("movies:list");
  });

  it("starts its requests and returns without waiting for them", () => {
    fetchJson.mockImplementation(() => new Promise(() => {}));
    const result = moviesLoader();
    expect(result).toEqual({ cached: true });
    expect(fetchJson).toHaveBeenCalledWith("/api/settings");
  });
});
