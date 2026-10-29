import { render, screen } from "@testing-library/react";
import { createMemoryRouter, RouterProvider, useLocation } from "react-router-dom";
import type { RouteObject } from "react-router-dom";
import { beforeEach, describe, expect, it, vi } from "vitest";

// #1392: where /recover sends a person on a fresh install, and that Settings, Account is a route of the settings area.

const status = vi.hoisted(() => ({ requiresSetup: false }));
vi.mock("./lib/bootstrap-status", () => ({ readRequiresSetup: async () => status.requiresSetup }));
vi.mock("./routes/recovery-page", () => ({ RecoveryPage: () => <p>The recovery page</p> }));
vi.mock("./routes/settings-account-page", () => ({ SettingsAccountPage: () => <p>The account page</p> }));

const { router } = await import("./router");

function Where() {
  const location = useLocation();
  return <p data-testid="where">{location.pathname + location.search}</p>;
}

const routes = router.routes as RouteObject[];
const recoverRoute = () => routes.find((route) => route.path === "/recover")!;
const findRoute = (list: RouteObject[], path: string): RouteObject | undefined => {
  for (const route of list) {
    if (route.path === path) return route;
    const inside = route.children ? findRoute(route.children, path) : undefined;
    if (inside) return inside;
  }
  return undefined;
};
const settingsRoute = () => findRoute(routes, "settings")!;
const accountRoute = () => settingsRoute().children!.find((route) => route.path === "account")!;

beforeEach(() => {
  status.requiresSetup = false;
});

describe("the recovery route", () => {
  it("is a page of its own, outside the app shell", () => {
    const route = recoverRoute();
    expect(route).toBeTruthy();
    expect(route.loader).toBeTypeOf("function");
    expect(route.errorElement).toBeTruthy();
    expect(route.hydrateFallbackElement).toBeTruthy();
  });

  it("shows the recovery page once the install has an account, signed in or not", async () => {
    const memory = createMemoryRouter(
      [{ path: "/recover", loader: recoverRoute().loader, element: recoverRoute().element }, { path: "*", element: <Where /> }],
      { initialEntries: ["/recover"] }
    );
    render(<RouterProvider router={memory} />);
    expect(await screen.findByText("The recovery page")).toBeTruthy();
  });

  it("sends a fresh install to setup, where there is no account to get back into", async () => {
    status.requiresSetup = true;
    const memory = createMemoryRouter(
      [{ path: "/recover", loader: recoverRoute().loader, element: recoverRoute().element }, { path: "*", element: <Where /> }],
      { initialEntries: ["/recover"] }
    );
    render(<RouterProvider router={memory} />);
    expect((await screen.findByTestId("where")).textContent).toBe("/setup");
  });
});

describe("the account settings route", () => {
  it("loads the account page lazily inside the settings area", async () => {
    const route = accountRoute();
    expect(route.lazy).toBeTypeOf("function");
    const memory = createMemoryRouter(
      [{ path: "/settings", children: [{ path: "account", lazy: route.lazy as never }] }],
      { initialEntries: ["/settings/account"] }
    );
    render(<RouterProvider router={memory} />);
    expect(await screen.findByText("The account page")).toBeTruthy();
  });
});
