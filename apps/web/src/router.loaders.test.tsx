import { render } from "@testing-library/react";
import { createMemoryRouter, RouterProvider } from "react-router-dom";
import type { RouteObject } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// #1329: the router's own loaders decide where a person lands before any screen draws: setup first, then sign-in, then the page asked for.

const mocks = vi.hoisted(() => ({
  setup: vi.fn(async () => false),
  stored: vi.fn((): { token: string | null; user: unknown } => ({ token: null, user: null }))
}));

vi.mock("./lib/bootstrap-status", () => ({ readRequiresSetup: mocks.setup }));
vi.mock("./lib/use-auth", async (importActual) => ({ ...(await importActual<typeof import("./lib/use-auth")>()), readStored: mocks.stored }));


// The Dashboard is only the page a redirect lands on here. Loading the real one would give the coverage report a copy of
// its file with almost nothing run, and v8's merge of that copy with the Dashboard's own tests' loses their branches.
vi.mock("./routes/dashboard-page", () => ({
  dashboardLoader: () => null,
  dashboardShouldRevalidate: () => false,
  DashboardPage: () => <p>The Dashboard</p>
}));
const { router } = await import("./router");

type Loader = (args: { request: Request; params: Record<string, string>; context?: unknown }) => Promise<unknown>;

const loaderOf = (path: string) => (router.routes as RouteObject[]).find((route) => route.path === path)!.loader as unknown as Loader;
const run = (path: string, url = `http://localhost${path}`) => loaderOf(path)({ request: new Request(url), params: {} });
const redirectOf = async (promise: Promise<unknown>) => {
  try {
    await promise;
  } catch (thrown) {
    const response = thrown as Response;
    return `${response.status} ${response.headers.get("Location")}`;
  }
  return "no redirect";
};

const signedIn = { token: "t", user: { id: "u1" } };

beforeEach(() => {
  mocks.setup.mockResolvedValue(false);
  mocks.stored.mockReturnValue({ token: null, user: null });
  window.history.replaceState(null, "", "/");
});
afterEach(() => vi.clearAllMocks());

describe("the page behind the shell", () => {
  it("lets a signed-in person through", async () => {
    mocks.stored.mockReturnValue(signedIn);
    expect(await run("/")).toBeNull();
  });

  it("sends a person who is not signed in to sign in, and brings them back to the page they asked for", async () => {
    expect(await redirectOf(run("/", "http://localhost/movies?filter=missing"))).toBe(`302 /login?return=${encodeURIComponent("/movies?filter=missing")}`);
  });

  it("sends a half signed-in person (a token and no profile, or the reverse) to sign in", async () => {
    mocks.stored.mockReturnValue({ token: "t", user: null });
    expect(await redirectOf(run("/"))).toContain("302 /login?return=");
    mocks.stored.mockReturnValue({ token: null, user: { id: "u1" } });
    expect(await redirectOf(run("/"))).toContain("302 /login?return=");
  });

  it("sends a new install to setup first, ahead of sign-in", async () => {
    mocks.setup.mockResolvedValue(true);
    mocks.stored.mockReturnValue(signedIn);
    expect(await redirectOf(run("/", "http://localhost/tv"))).toBe(`302 /setup?return=${encodeURIComponent("/tv")}`);
  });

  it("keeps the # part of the page being opened, which a loader's request never carries", async () => {
    window.history.replaceState(null, "", "/tv#season-2");
    expect(await redirectOf(run("/", "http://localhost/tv"))).toBe(`302 /login?return=${encodeURIComponent("/tv#season-2")}`);
  });

  it("does not borrow the # part of a different page", async () => {
    window.history.replaceState(null, "", "/movies#other");
    expect(await redirectOf(run("/", "http://localhost/tv"))).toBe(`302 /login?return=${encodeURIComponent("/tv")}`);
  });

  it("prefers the # part the request itself carries", async () => {
    window.history.replaceState(null, "", "/tv#from-bar");
    expect(await redirectOf(run("/", "http://localhost/tv#own"))).toBe(`302 /login?return=${encodeURIComponent("/tv#own")}`);
  });
});

describe("the sign-in page", () => {
  it("opens for a person who is not signed in", async () => {
    expect(await run("/login")).toBeNull();
  });

  it("sends a new install to setup", async () => {
    mocks.setup.mockResolvedValue(true);
    expect(await redirectOf(run("/login"))).toBe("302 /setup");
  });

  it("sends a person who is already signed in to the dashboard", async () => {
    mocks.stored.mockReturnValue(signedIn);
    expect(await redirectOf(run("/login"))).toBe("302 /");
  });

  it("still opens for a half signed-in person", async () => {
    mocks.stored.mockReturnValue({ token: "t", user: null });
    expect(await run("/login")).toBeNull();
    mocks.stored.mockReturnValue({ token: null, user: { id: "u1" } });
    expect(await run("/login")).toBeNull();
  });
});

describe("the recovery page", () => {
  it("opens once the install is set up", async () => {
    expect(await run("/recover")).toBeNull();
  });

  it("sends a new install to setup", async () => {
    mocks.setup.mockResolvedValue(true);
    expect(await redirectOf(run("/recover"))).toBe("302 /setup");
  });
});

describe("the setup page", () => {
  it("opens while the install still needs setting up", async () => {
    mocks.setup.mockResolvedValue(true);
    expect(await run("/setup")).toBeNull();
  });

  it("sends a set-up install on to the page it was asked to return to", async () => {
    expect(await redirectOf(run("/setup", "http://localhost/setup?return=%2Ftv"))).toBe("302 /tv");
  });

  it("sends a set-up install to sign in when the return address is missing or points to another site", async () => {
    expect(await redirectOf(run("/setup"))).toBe("302 /login");
    expect(await redirectOf(run("/setup", "http://localhost/setup?return=%2F%2Fevil.example"))).toBe("302 /login");
  });
});

describe("every page the shell holds", () => {
  it("loads each lazy page with the skeleton's error screen and pending screen", async () => {
    const lazy: RouteObject[] = [];
    const walk = (routes: readonly RouteObject[]) => {
      for (const route of routes) {
        if (route.lazy) lazy.push(route);
        if (route.children) walk(route.children);
      }
    };
    walk(router.routes as RouteObject[]);
    expect(lazy.length).toBeGreaterThan(50);
    for (const route of lazy) {
      const loaded = await (route.lazy as () => Promise<RouteObject>)();
      expect(loaded.Component).toBeTypeOf("function");
      expect(loaded.ErrorBoundary).toBeTypeOf("function");
      expect(loaded.HydrateFallback).toBeTypeOf("function");
    }
  }, 120000);

  it("sends an old show address to the show's page", () => {
    const shell = (router.routes as RouteObject[]).find((route) => route.path === "/")!;
    const legacy = shell.children!.find((route) => route.path === "shows/:id")!;
    const memory = createMemoryRouter([{ path: "shows/:id", element: legacy.element }, { path: "tv/:id", element: <p>show page</p> }], { initialEntries: ["/shows/abc"] });
    render(<RouterProvider router={memory} />);
    expect(memory.state.location.pathname).toBe("/tv/abc");
  });
});

describe("the old Automation address", () => {
  it("opens the schedule with the library the link named", () => {
    const find = (routes: readonly RouteObject[]): RouteObject | undefined => {
      for (const route of routes) {
        if (route.path === "automation") return route;
        const inside = route.children ? find(route.children) : undefined;
        if (inside) return inside;
      }
      return undefined;
    };
    const old = find(router.routes as RouteObject[])!;
    const memory = createMemoryRouter([{ path: "automation", element: old.element }, { path: "search-cycles", element: <p>schedule</p> }], { initialEntries: ["/automation?library=lib1"] });
    render(<RouterProvider router={memory} />);
    expect(memory.state.location.pathname + memory.state.location.search).toBe("/search-cycles?library=lib1");
  });
});
