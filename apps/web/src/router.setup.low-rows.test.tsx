import { render, screen, waitFor } from "@testing-library/react";
import { createMemoryRouter, RouterProvider, useLocation } from "react-router-dom";
import type { RouteObject } from "react-router-dom";
import { describe, expect, it, vi } from "vitest";

// #1329: where /setup sends a person once an account exists, and what the lazily loaded setup guide shows while it loads.

vi.mock("./lib/bootstrap-status", () => ({ readRequiresSetup: async () => false }));

const guideLoader = vi.hoisted(() => ({ release: null as null | (() => void) }));
vi.mock("./routes/setup-guide-page", () => ({
  setupGuideLoader: () => new Promise((resolve) => { guideLoader.release = () => resolve({ ready: true }); }),
  SetupGuidePage: () => <p>The setup guide</p>
}));

const { router } = await import("./router");

function Where() {
  const location = useLocation();
  return <p data-testid="where">{location.pathname + location.search}</p>;
}

const topLevel = (path: string) => (router.routes as RouteObject[]).find((route) => route.path === path)!;
const guideParent = () => (router.routes as RouteObject[]).find((route) => route.children?.some((child) => child.path === "setup-guide"))!;
const guideRoute = () => guideParent().children!.find((route) => route.path === "setup-guide")!;

async function endsAt(address: string): Promise<string> {
  const memory = createMemoryRouter(
    [
      { path: "/setup", loader: topLevel("/setup").loader, element: <p>The setup page</p> },
      { path: "*", element: <Where /> }
    ],
    { initialEntries: [address] }
  );
  render(<RouterProvider router={memory} />);
  return (await screen.findByTestId("where")).textContent ?? "";
}

describe("/setup once an account already exists, low rows (#1329)", () => {
  it("goes to the return address when it starts with a slash", async () => {
    expect(await endsAt("/setup?return=%2Fmovies%3Fq%3Ddune")).toBe("/movies?q=dune");
  });

  it.each([
    ["no return address", "/setup", "/login"],
    ["a return address on another site", "/setup?return=https%3A%2F%2Fevil.example%2F", "/login"],
    ["a protocol-relative address", "/setup?return=%2F%2Fevil.example", "/login"]
  ])("goes to the sign-in page for %s", async (_name, address, expected) => {
    expect(await endsAt(address)).toBe(expected);
  });
});

describe("the setup guide route, low rows (#1329)", () => {
  it("shows the loading skeleton while the lazy module and its loader run, then the guide", async () => {
    const route = guideRoute();
    expect(route.lazy).toBeTruthy();
    const parent = guideParent();
    expect(parent.hydrateFallbackElement).toBeTruthy();
    const memory = createMemoryRouter([{ path: "/", hydrateFallbackElement: parent.hydrateFallbackElement, children: [{ path: "setup-guide", lazy: route.lazy as never }] }], { initialEntries: ["/setup-guide"] });

    const view = render(<RouterProvider router={memory} />);

    await waitFor(() => expect(guideLoader.release).not.toBeNull());
    expect(view.container.querySelector("[data-route-skeleton]")).not.toBeNull();
    expect(screen.queryByText("The setup guide")).toBeNull();

    guideLoader.release!();

    expect(await screen.findByText("The setup guide")).toBeTruthy();
    expect(view.container.querySelector("[data-route-skeleton]")).toBeNull();
  });
});
