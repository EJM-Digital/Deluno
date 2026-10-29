import { render, screen, waitFor } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import type { RouteObject } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";

// #1329 (movie-detail and show-detail, route loading): a title's page is a lazy route. While its module and its loader are on the
// way the person sees the busy placeholder (never a blank page or half a title), and the page itself only draws once the loader
// has answered.

vi.mock("./lib/bootstrap-status", () => ({ readRequiresSetup: async () => false }));

const { router } = await import("./router");

function find(routes: readonly RouteObject[], pattern: string, parent = ""): RouteObject | undefined {
  for (const route of routes) {
    const here = route.path ? `${parent.replace(/\/$/, "")}/${route.path.replace(/^\//, "")}` : parent || "/";
    if (here === pattern && route.lazy) return route;
    const inside = route.children ? find(route.children, pattern, here) : undefined;
    if (inside) return inside;
  }
  return undefined;
}

describe.each([
  ["/movies/:id", "/movies/some-film", "a film"],
  ["/tv/:id", "/tv/some-show", "a show"]
])("%s (%s)", (pattern, address, what) => {
  it(`shows the busy placeholder while ${what}'s page module is on the way and its loader has not answered`, { timeout: 60000 }, async () => {
    const route = find(router.routes as RouteObject[], pattern);
    expect(route, `${pattern} is a lazy route`).toBeDefined();
    expect(route!.lazy).toBeTypeOf("function");

    const loaded = await (route!.lazy as () => Promise<RouteObject>)();
    expect(loaded.HydrateFallback).toBeTruthy();
    expect(loaded.ErrorBoundary).toBeTruthy();

    const pending = createMemoryRouter(
      [{ path: pattern, loader: () => new Promise(() => undefined), Component: () => <p>the title page</p>, HydrateFallback: loaded.HydrateFallback }],
      { initialEntries: [address] }
    );
    const view = render(<RouterProvider router={pending} />);

    expect(view.container.querySelector("[aria-busy='true']")).not.toBeNull();
    expect(screen.queryByText("the title page")).not.toBeInTheDocument();
  });

  it(`draws the busy placeholder, not ${what}'s page, when the loader answered nothing`, { timeout: 60000 }, async () => {
    const route = find(router.routes as RouteObject[], pattern)!;
    const loaded = await (route.lazy as () => Promise<RouteObject>)();
    const Page = loaded.Component!;
    const nothing = createMemoryRouter(
      [{ path: pattern, loader: () => null, Component: Page, HydrateFallback: loaded.HydrateFallback }],
      { initialEntries: [address] }
    );
    const view = render(<RouterProvider router={nothing} />);

    await waitFor(() => expect(view.container.querySelector("[aria-busy='true']")).not.toBeNull());
  });
});
