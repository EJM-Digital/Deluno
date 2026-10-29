import { act, render, waitFor } from "@testing-library/react";
import { Outlet, RouterProvider } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";

/**
 * The legacy TV addresses (coverage register, area show-detail, routes and redirects): the old /shows family and the
 * /tv/import and /tv/library sub-pages land on the TV shelf or the show's page, keeping the id.
 */
vi.mock("./lib/use-auth", async (importOriginal) => ({
  ...(await importOriginal<typeof import("./lib/use-auth")>()),
  readStored: () => ({ token: "t", user: { id: "u" } })
}));
vi.mock("./lib/bootstrap-status", async (importOriginal) => ({
  ...(await importOriginal<typeof import("./lib/bootstrap-status")>()),
  readRequiresSetup: async () => false
}));
vi.mock("./layouts/app-layout", () => ({ AppLayout: () => <Outlet /> }));
vi.mock("./components/app/media-workspace-shell", () => ({
  MoviesWorkspaceLayout: () => <Outlet />,
  TvWorkspaceLayout: () => <Outlet />
}));
vi.mock("./routes/library-page", () => ({ showsLoader: () => null, moviesLoader: () => null, ShowsPage: () => <p>TV shelf</p>, MoviesPage: () => <p>Movie shelf</p> }));
vi.mock("./routes/show-detail-page", () => ({ showDetailLoader: () => null, ShowDetailPage: () => <p>Show page</p> }));

async function openLegacy(path: string) {
  const { router } = await import("./router");
  await act(async () => { await router.navigate(path); });
  const view = render(<RouterProvider router={router} />);
  await waitFor(() => expect(router.state.navigation.state).toBe("idle"));
  view.unmount();
  return `${router.state.location.pathname}${router.state.location.search}`;
}

describe("legacy TV addresses", () => {
  beforeEach(() => { vi.resetModules(); });

  it.each(["/shows", "/shows/library", "/tv/import", "/tv/library"])("%s redirects to /tv", async (path) => {
    expect(await openLegacy(path)).toBe("/tv");
  });

  it("/shows/:id redirects to /tv/:id and keeps the id", async () => {
    expect(await openLegacy("/shows/show-42")).toBe("/tv/show-42");
  });
});
