import { cleanup, render, screen } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import type { RouteObject } from "react-router-dom";
import { afterEach, describe, expect, it, vi } from "vitest";

// #1329 (settings-library, settings-lists, automation): when a screen's loader cannot read what it needs (the server does not answer),
// the route shows the route error state, "This area could not load", with a way to try again, never a blank page and never the
// half-built screen. Each address below is a real route of the app's table; its own lazy module, loader and error screen are used.

vi.mock("./lib/bootstrap-status", () => ({ readRequiresSetup: async () => false }));
vi.mock("./lib/use-auth", async (importActual) => ({
  ...(await importActual<typeof import("./lib/use-auth")>()),
  authedFetch: vi.fn(async () => {
    throw new Error("The server did not answer.");
  })
}));

const { router } = await import("./router");

function find(routes: readonly RouteObject[], address: string, parent = ""): RouteObject | undefined {
  for (const route of routes) {
    const here = route.path ? `${parent.replace(/\/$/, "")}/${route.path.replace(/^\//, "")}` : parent || "/";
    if (here === address && route.lazy) return route;
    const inside = route.children ? find(route.children, address, here) : undefined;
    if (inside) return inside;
  }
  return undefined;
}

afterEach(() => cleanup());

describe("a loader that cannot read its screen's data", () => {
  it.each([
    "/settings",
    "/settings/lists",
    "/settings/metadata",
    "/settings/auto-tagging",
    "/settings/tags",
    "/settings/general",
    "/settings/notifications",
    "/settings/ui",
    "/search-cycles",
    "/search-cycles/retention",
    "/search-cycles/download-health",
    "/search-cycles/keep-seeding"
  ])("%s shows 'This area could not load' with Try again, not a blank page or the half-built screen", { timeout: 60000 }, async (address) => {
    const route = find(router.routes as RouteObject[], address);
    expect(route, `${address} is a lazy route of the table`).toBeDefined();
    const loaded = await (route!.lazy as () => Promise<RouteObject>)();
    vi.spyOn(console, "error").mockImplementation(() => undefined);
    const failing = createMemoryRouter(
      [{ path: "/", loader: loaded.loader, Component: loaded.Component, ErrorBoundary: loaded.ErrorBoundary, HydrateFallback: loaded.HydrateFallback }],
      { initialEntries: ["/"] }
    );
    render(<RouterProvider router={failing} />);

    expect(await screen.findByText("This area could not load", {}, { timeout: 10000 })).toBeVisible();
    expect(screen.getByRole("button", { name: /try again/i })).toBeVisible();
    vi.restoreAllMocks();
  });
});
