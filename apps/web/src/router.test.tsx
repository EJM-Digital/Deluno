import { render, screen, waitFor } from "@testing-library/react";
import { createMemoryRouter, MemoryRouter, Route, RouterProvider, Routes, useLocation } from "react-router-dom";
import type { RouteObject } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";

// #1329: the router's own route table, as built for the app. Redirects are run by rendering the table's own elements.

vi.mock("./lib/bootstrap-status", () => ({ readRequiresSetup: async () => false }));

const { router } = await import("./router");

type TableRoute = { pattern: string; route: RouteObject };

/** Every route of the app's table with its full path, so a test can look up the route a person's address matches. */
function flatten(routes: readonly RouteObject[], parent = ""): TableRoute[] {
  const out: TableRoute[] = [];
  for (const route of routes) {
    const here = route.path ? `${parent.replace(/\/$/, "")}/${route.path.replace(/^\//, "")}` : parent || "/";
    out.push({ pattern: here, route });
    if (route.children) out.push(...flatten(route.children, here));
  }
  return out;
}

const table = flatten(router.routes as RouteObject[]);
const routeFor = (pattern: string) => {
  const found = table.find((entry) => entry.pattern === pattern && entry.route.element);
  if (!found) throw new Error(`No route ${pattern} in the table`);
  return found.route;
};

function Where() {
  const location = useLocation();
  return <p data-testid="where">{location.pathname + location.search}</p>;
}

/** Opens `address` against the route the table has at `pattern`, and says where the person ends up. */
function endsAt(pattern: string, address: string, stops: string[] = []): string {
  const route = routeFor(pattern);
  const view = render(
    <MemoryRouter initialEntries={[address]}>
      <Routes>
        <Route path={pattern} element={route.element} />
        {stops.map((stop) => <Route key={stop} path={stop} element={<Where />} />)}
        <Route path="*" element={<Where />} />
      </Routes>
    </MemoryRouter>
  );
  const where = screen.getByTestId("where").textContent ?? "";
  view.unmount();
  return where;
}

describe("the old addresses still lead somewhere (#1329)", () => {
  it.each([
    ["/movies/import", "/movies/import", "/movies"],
    ["/movies/library", "/movies/library", "/movies"],
    ["/tv/import", "/tv/import", "/tv"],
    ["/tv/library", "/tv/library", "/tv"]
  ])("%s lands on the library", (pattern, address, expected) => {
    expect(endsAt(pattern, address)).toBe(expected);
  });

  it("/shows and /shows/library go to /tv, and /shows/:id goes to /tv/:id with the id kept", () => {
    expect(endsAt("/shows", "/shows")).toBe("/tv");
    expect(endsAt("/shows/library", "/shows/library")).toBe("/tv");
    expect(endsAt("/shows/:id", "/shows/series-42")).toBe("/tv/series-42");
  });

  it.each([
    ["/settings/media", "/settings/media-management"],
    ["/settings/root-folders", "/settings/destination-rules"],
    ["/settings/quality-sizes", "/settings/quality"],
    ["/settings/indexers", "/indexers/indexers"],
    ["/settings/download-clients", "/indexers/download-clients"],
    ["/settings/import-lists", "/settings/lists"],
    ["/settings/connect", "/indexers"]
  ])("%s goes to %s", (address, expected) => {
    expect(endsAt(address, address)).toBe(expected);
  });

  it("/indexers goes to /indexers/indexers", () => {
    expect(endsAt("/indexers", "/indexers")).toBe("/indexers/indexers");
  });

  it("an unknown /settings/<anything> goes to /settings", () => {
    expect(endsAt("/settings/*", "/settings/never-heard-of-it", ["/settings"])).toBe("/settings");
    expect(endsAt("/settings/*", "/settings/a/deeper/one", ["/settings"])).toBe("/settings");
  });
});

describe("the sign-in and setup pages' first paint and failure (#1329)", () => {
  const standalone = (path: string) => (router.routes as RouteObject[]).find((route) => route.path === path)!;

  it.each(["/login", "/setup"])("%s shows the loading skeleton while its first answer is awaited", async (path) => {
    const route = standalone(path);
    expect(route.hydrateFallbackElement).toBeTruthy();
    const pending = createMemoryRouter(
      [{ path, loader: () => new Promise(() => undefined), element: <p>the page</p>, hydrateFallbackElement: route.hydrateFallbackElement }],
      { initialEntries: [path] }
    );
    const view = render(<RouterProvider router={pending} />);

    expect(view.container.querySelector("[data-route-skeleton]")).not.toBeNull();
    expect(screen.queryByText("the page")).not.toBeInTheDocument();
  });

  it.each(["/login", "/setup"])("%s shows the error screen, not a blank page, when its loader fails", async (path) => {
    const route = standalone(path);
    expect(route.errorElement).toBeTruthy();
    vi.spyOn(console, "error").mockImplementation(() => undefined);
    const failing = createMemoryRouter(
      [{ path, loader: () => { throw new Error("the loader broke"); }, element: <p>the page</p>, errorElement: route.errorElement }],
      { initialEntries: [path] }
    );
    render(<RouterProvider router={failing} />);

    await waitFor(() => expect(screen.getByRole("button", { name: /try again/i })).toBeInTheDocument());
    expect(screen.queryByText("the page")).not.toBeInTheDocument();
    vi.restoreAllMocks();
  });
});

describe("a screen's own revalidation rule survives the loading wrapper (#1329)", () => {
  it("the Dashboard route still declines the revalidations its own module declines", async () => {
    const dashboard = table.find((entry) => entry.pattern === "/" && entry.route.index)!.route;
    expect(dashboard.lazy).toBeTypeOf("function");
    const { dashboardShouldRevalidate } = await import("./routes/dashboard-page");

    const loaded = await (dashboard.lazy as () => Promise<RouteObject>)();

    expect(loaded.shouldRevalidate).toBe(dashboardShouldRevalidate);
    expect(loaded.HydrateFallback).toBeTruthy();
    expect(loaded.ErrorBoundary).toBeTruthy();
  });

  it("a screen that declines nothing gets no revalidation rule of its own", async () => {
    const movies = table.find((entry) => entry.pattern === "/movies" && entry.route.index)!.route;
    const loaded = await (movies.lazy as () => Promise<RouteObject>)();
    expect(loaded.shouldRevalidate).toBeUndefined();
  });
});

describe("a signed-out visit to a System page (#1329)", () => {
  it.each(["/system/audit", "/system/recycle-bin", "/system/security"])("%s goes to the sign-in page and brings the person back to it afterwards", async (path) => {
    window.localStorage.clear();
    window.sessionStorage.clear();
    const visit = createMemoryRouter(router.routes as RouteObject[], { initialEntries: [path] });

    await waitFor(() => expect(visit.state.initialized).toBe(true), { timeout: 8000 });
    await waitFor(() => expect(visit.state.location.pathname).toBe("/login"), { timeout: 8000 });
    expect(visit.state.location.search).toBe(`?return=${encodeURIComponent(path)}`);
    visit.dispose();
  });
});
