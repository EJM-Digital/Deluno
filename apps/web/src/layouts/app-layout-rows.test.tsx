import { act, cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { createMemoryRouter, RouterProvider, useLocation } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { queryClient } from "../lib/route-cache";

/**
 * What the app shell does around every screen: the keyboard, the theme button, the phone search sheet, the toast when an
 * import ends anywhere, and the one re-read after a reconnect that could not replay what was missed. The shell is the real
 * AppLayout; only the network, the live connection and the sign-in are stood in for.
 */

const live = vi.hoisted(() => ({
  handlers: new Map<string, Set<(payload: any) => void>>(),
  resync: null as null | (() => void),
  theme: { resolvedTheme: "light" as string, setTheme: null as any },
  attentionRefresh: null as any,
  authedFetch: null as any,
  token: "t" as string | null,
  host: null as string | null,
  prefetch: null as any,
  toast: { success: null as any, error: null as any }
}));

vi.mock("../lib/use-signalr", async (importOriginal) => {
  const { useEffect, useRef } = await import("react");
  const actual = await importOriginal<typeof import("../lib/use-signalr")>();
  return {
    ...actual,
    SignalRProvider: ({ children }: { children: React.ReactNode }) => children,
    useSignalRStatus: () => "connected",
    useSignalRResync: (callback: () => void) => { live.resync = callback; },
    useSignalREvent: (name: string, _subjects: unknown, handler: (payload: unknown) => void) => {
      const latest = useRef(handler);
      useEffect(() => { latest.current = handler; });
      useEffect(() => {
        const call = (payload: unknown) => latest.current(payload);
        const set = live.handlers.get(name) ?? new Set();
        set.add(call);
        live.handlers.set(name, set);
        return () => { set.delete(call); };
      }, [name]);
    }
  };
});

vi.mock("../lib/use-auth", async (importOriginal) => ({
  ...(await importOriginal<typeof import("../lib/use-auth")>()),
  useAuth: () => ({ token: live.token, user: { displayName: "Sam Example", username: "sam", avatarInitials: "SE" }, logout: vi.fn() }),
  authedFetch: (...args: unknown[]) => live.authedFetch(...args)
}));

vi.mock("../lib/use-attention", () => ({
  useAttention: () => ({
    failedJobs: 0, failedTitleJobs: 0, failedBackgroundJobs: 0, deadLetteredJobs: 0, indexerAlerts: 0, downloadsNeedingAction: 0,
    failedImportJobs: 0, movieWanted: 0, tvWanted: 0, healthProblems: 0, total: 0, summary: {}, loading: false,
    refresh: (...args: unknown[]) => live.attentionRefresh(...args)
  })
}));
vi.mock("../lib/use-host-name", () => ({ hostTitle: (name: string | null) => (name ? `Deluno · ${name}` : "Deluno"), useHostName: () => live.host }));
vi.mock("../lib/use-cut-value-titles", () => ({ useCutValueTitles: () => {} }));
vi.mock("../lib/route-prefetch", () => ({ prefetchMainRoutesWhenIdle: () => live.prefetch(), prefetchHandlers: () => ({}) }));
vi.mock("../lib/display-preferences", () => ({ DisplayPreferencesProvider: ({ children }: { children: React.ReactNode }) => children }));
vi.mock("../components/shell/notifications-bell", () => ({ NotificationsBell: () => null }));
vi.mock("../components/app/outage-banner", () => ({ OutageBanner: () => null }));
vi.mock("../components/shell/ws-status-badge", () => ({ WsStatusBadge: () => null }));
vi.mock("../components/shell/toaster", async () => ({
  Toaster: () => null,
  realtimeToast: {
    success: (...args: unknown[]) => live.toast.success(...args),
    error: (...args: unknown[]) => live.toast.error(...args)
  }
}));
vi.mock("next-themes", () => ({
  useTheme: () => ({ resolvedTheme: live.theme.resolvedTheme, setTheme: (...args: unknown[]) => live.theme.setTheme(...args) })
}));

import { AppLayout } from "./app-layout";

// The command palette (cmdk) measures its list; jsdom has neither of these.
globalThis.ResizeObserver ??= class { observe() {} unobserve() {} disconnect() {} } as unknown as typeof ResizeObserver;
Element.prototype.scrollIntoView ??= () => {};

function Where() {
  const location = useLocation();
  return <output data-testid="where">{location.pathname}</output>;
}

let loads = 0;

async function mount(path = "/") {
  const router = createMemoryRouter(
    [{
      path: "/",
      element: <AppLayout />,
      loader: () => { loads += 1; return null; },
      children: [
        { index: true, element: <><Where /><input aria-label="A text box" /></> },
        { path: "*", element: <><Where /><input aria-label="A text box" /></>, loader: () => null }
      ]
    }],
    { initialEntries: [path] }
  );
  render(<RouterProvider router={router} />);
  await screen.findByTestId("where");
  return router;
}

const where = () => screen.getByTestId("where").textContent;
const press = (key: string, init: KeyboardEventInit = {}, target: Element | Window = window) =>
  act(() => { fireEvent.keyDown(target, { key, ...init }); });
/** Lets a navigation that was asked for finish (the router reads the next screen first), so a test that expects none can tell. */
const settle = () => act(async () => { await new Promise((resolve) => setTimeout(resolve, 80)); });
const sendEvent = (name: string, payload: unknown) => act(() => { for (const handler of live.handlers.get(name) ?? []) handler(payload); });

beforeEach(() => {
  live.handlers.clear();
  live.resync = null;
  live.theme = { resolvedTheme: "light", setTheme: vi.fn() };
  live.attentionRefresh = vi.fn();
  live.authedFetch = vi.fn(() => Promise.resolve(new Response("{}", { status: 200 })));
  live.toast = { success: vi.fn(), error: vi.fn() };
  loads = 0;
  live.token = "t";
  live.host = null;
  live.prefetch = vi.fn();
  localStorage.clear();
  queryClient.clear();
});
afterEach(() => { vi.useRealTimers(); });


const openPalette = async () => {
  fireEvent.keyDown(document.body, { key: "k", code: "KeyK", ctrlKey: true });
  return screen.findByRole("dialog");
};

describe("the command palette's recents and Preferences entry (#1329)", () => {
  const pick = async (label: string) => {
    const dialog = await openPalette();
    fireEvent.click(within(dialog).getAllByText(label).at(-1)!);
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    await settle();
  };

  it("lists the last 5 distinct destinations first, newest first, and a recent reopens its page", async () => {
    await mount("/");
    for (const label of ["Movies", "TV", "Discover", "Movies", "Transfers", "Collections", "Schedule"]) await pick(label);

    const dialog = await openPalette();
    const recents = within(dialog).getByText("Recents").parentElement!;
    const paths = Array.from(recents.querySelectorAll("[cmdk-item]")).map((item) => item.querySelector(".font-mono")?.textContent);
    expect(paths).toEqual(["/calendar", "/collections", "/queue", "/movies", "/discover"]);

    fireEvent.click(within(recents).getByText("/collections").closest("[cmdk-item]")!);
    await waitFor(() => expect(where()).toBe("/collections"));
  });

  it("a Settings entry in the recents is labelled with its path", async () => {
    await mount("/");
    const dialog = await openPalette();
    const settingsItem = Array.from(dialog.querySelectorAll("[cmdk-group]")).find((group) => group.textContent?.startsWith("Settings"))!.querySelector("[cmdk-item]")!;
    const label = settingsItem.textContent!;
    fireEvent.click(settingsItem);
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    await settle();
    const target = where()!;

    const reopened = await openPalette();
    const recents = within(reopened).getByText("Recents").parentElement!;
    const entry = recents.querySelector("[cmdk-item]")!;
    expect(entry.querySelector("span")!.textContent).toBe(target);
    expect(label).not.toBe(target);
  });

  it("never lists Add movie, Add TV show, Add indexer or Refresh data, because the layout supplies no handler for them", async () => {
    await mount("/");
    const dialog = await openPalette();
    for (const name of ["Add movie", "Add TV show", "Add indexer", "Refresh data"]) expect(within(dialog).queryByText(name)).toBeNull();
    expect(within(dialog).getByText("Preferences")).toBeTruthy();
  });

  it("the Preferences entry reads 'Switch to light mode' in a dark theme and saves the change as the topbar button does", async () => {
    live.theme.resolvedTheme = "dark";
    await mount("/");
    const dialog = await openPalette();

    fireEvent.click(within(dialog).getByText("Switch to light mode"));

    expect(live.theme.setTheme).toHaveBeenCalledWith("light");
    await waitFor(() => expect(live.authedFetch).toHaveBeenCalledWith("/api/settings", expect.objectContaining({ method: "PATCH", body: JSON.stringify({ uiTheme: "light" }) })));
  });

  it("the Preferences entry reads 'Switch to dark mode' in a light theme and saves dark", async () => {
    live.theme.resolvedTheme = "light";
    await mount("/");
    const dialog = await openPalette();

    fireEvent.click(within(dialog).getByText("Switch to dark mode"));

    expect(live.theme.setTheme).toHaveBeenCalledWith("dark");
    await waitFor(() => expect(live.authedFetch).toHaveBeenCalledWith("/api/settings", expect.objectContaining({ body: JSON.stringify({ uiTheme: "dark" }) })));
  });
});

describe("the sidebar's links, chevrons and rail (#1329)", () => {
  const nav = (label: string) => screen.getByRole("navigation", { name: label });

  it.each([
    ["Media Management", "/settings/libraries"],
    ["Quality & Release", "/settings/policy-sets"],
    ["Automation & Recovery", "/search-cycles"],
    ["Discover Media", "/settings/lists"]
  ])("the Setup link %s opens from a click", async (label, path) => {
    await mount("/");
    const link = within(nav("Media Management")).getByRole("link", { name: label });
    expect(link.getAttribute("href")).toContain(path);

    fireEvent.click(link);

    await waitFor(() => expect(where()!.startsWith(path)).toBe(true));
  });

  it("Deluno > Preferences opens from a click", async () => {
    await mount("/");
    const link = within(nav("System controls")).getByRole("link", { name: "Preferences" });

    fireEvent.click(link);

    await waitFor(() => expect(where()).toBe(new URL(link.getAttribute("href")!, "http://x").pathname));
    expect(where()!.startsWith("/settings")).toBe(true);
  });

  it("every area keeps its pages as tabs in the page toolbar, so the sidebar draws no expand or collapse chevron", async () => {
    await mount("/");
    expect(screen.queryAllByRole("button", { name: /^(Expand|Collapse) / }).filter((button) => button.getAttribute("aria-expanded") !== null && !/navigation$/.test(button.getAttribute("aria-label") ?? ""))).toHaveLength(0);
  });

  it("the page and footer move with the sidebar: collapsing sets --sidebar-width to 84px and expanding takes it away", async () => {
    await mount("/");
    const wrapper = () => document.querySelector("aside")!.parentElement as HTMLElement;
    expect(wrapper().style.getPropertyValue("--sidebar-width")).toBe("");

    fireEvent.click(screen.getByRole("button", { name: "Collapse navigation" }));
    expect(wrapper().style.getPropertyValue("--sidebar-width")).toBe("84px");

    fireEvent.click(screen.getByRole("button", { name: "Expand navigation" }));
    expect(wrapper().style.getPropertyValue("--sidebar-width")).toBe("");
  });

  it("between 1024 and 1099 px the menu is the rail by itself, and a click on Expand overrides it and is remembered", async () => {
    const original = window.matchMedia;
    window.matchMedia = ((query: string) => ({
      matches: query.includes("min-width: 1024px") && query.includes("max-width: 1099px"),
      media: query, addEventListener: () => undefined, removeEventListener: () => undefined, addListener: () => undefined, removeListener: () => undefined, onchange: null, dispatchEvent: () => false
    })) as unknown as typeof window.matchMedia;
    try {
      await mount("/");
      expect(screen.getByRole("button", { name: "Expand navigation" })).toBeTruthy();
      expect((document.querySelector("aside")!.parentElement as HTMLElement).style.getPropertyValue("--sidebar-width")).toBe("84px");

      fireEvent.click(screen.getByRole("button", { name: "Expand navigation" }));
      expect(screen.getByRole("button", { name: "Collapse navigation" })).toBeTruthy();
      expect(localStorage.getItem("deluno.sidebar.collapsed")).toBe("false");
    } finally {
      window.matchMedia = original;
    }
  });

  it("outside that width range the menu stays full on its own", async () => {
    await mount("/");
    expect(screen.getByRole("button", { name: "Collapse navigation" })).toBeTruthy();
  });
});

describe("the skip link, the tab title and the first fetch (#1329)", () => {
  it("'Skip to content' is hidden until it has keyboard focus, and jumps to the main content", async () => {
    await mount("/movies");
    const link = screen.getByRole("link", { name: "Skip to content" });

    expect(link.getAttribute("href")).toBe("#main-content");
    expect(link.className).toContain("sr-only");
    expect(link.className).toContain("focus:not-sr-only");
    expect(document.getElementById("main-content")).not.toBeNull();
    expect(document.getElementById("main-content")!.tagName).toBe("MAIN");
  });

  it("the tab is titled 'Deluno · <computer name>' once the name is known, and goes back to plain 'Deluno' when the shell goes away", async () => {
    live.host = "OFFICE-PC";
    const router = createMemoryRouter([{ path: "/", element: <AppLayout />, children: [{ index: true, element: <Where /> }] }]);
    const shell = render(<RouterProvider router={router} />);
    await screen.findByTestId("where");

    expect(document.title).toBe("Deluno · OFFICE-PC");
    shell.unmount();
    expect(document.title).toBe("Deluno");
  });

  it("the tab is plain 'Deluno' while the computer's name is not known", async () => {
    live.host = null;
    await mount("/");
    expect(document.title).toBe("Deluno");
  });

  it("the main screens are fetched when the app is idle after sign-in", async () => {
    await mount("/");
    expect(live.prefetch).toHaveBeenCalledTimes(1);
  });

  it("nothing is fetched ahead for a visit with no sign-in", async () => {
    live.token = null;
    await mount("/");
    expect(live.prefetch).not.toHaveBeenCalled();
  });

  it("signing out empties the shared data cache, so the next person never sees the last one's data", async () => {
    await mount("/");
    queryClient.setQueryData(["src", "movies"], [{ title: "The last person's film" }]);
    expect(queryClient.getQueryData(["src", "movies"])).toBeDefined();

    live.token = null;
    cleanup();
    await mount("/");

    expect(queryClient.getQueryData(["src", "movies"])).toBeUndefined();
  });
});

describe("each route names itself in the topbar (#1329)", () => {
  const title = () => screen.queryAllByRole("heading", { level: 1 })[0]?.textContent;

  it.each([
    ["/collections", "Collections"],
    ["/discover", "Discover"],
    ["/search", "Search"],
    ["/settings/quality", "Quality & Release"],
    ["/settings/lists", "Discover Media"],
    ["/settings/general", "Preferences"],
    ["/setup-guide", "Guided setup"],
    ["/search-cycles", "Automation & Recovery"]
  ])("%s is titled %s", async (path, expected) => {
    await mount(path);
    expect(title()).toBe(expected);
  });

  it.each(["/queue", "/queue?view=handoffs", "/queue?view=history&status=failed"])("%s is Transfers, which holds what Activity was, and says so", async (path) => {
    await mount(path);
    expect(title()).toBe("Transfers");
    expect(screen.getByText("Downloads, hand-offs to the processor, and the record of what happened")).toBeInTheDocument();
  });

  it("a film or show page leaves the heading to the page, but a named sub-route of it is an ordinary page", async () => {
    await mount("/movies/movie-1");
    expect(title()).toBeUndefined();

    cleanup();
    await mount("/movies/wanted");
    expect(title()).toBe("Movies");
  });
});

describe("a value cut with an ellipsis (#1329)", () => {
  it("is given its full text as a tooltip", async () => {
    // The hook runs at the top of AppLayout; the shell tests stand it in for a no-op, so this one uses the real one.
    const { useCutValueTitles } = await vi.importActual<typeof import("../lib/use-cut-value-titles")>("../lib/use-cut-value-titles");
    function Probe() {
      useCutValueTitles();
      return <p className="truncate" style={{ textOverflow: "ellipsis", overflow: "hidden", whiteSpace: "nowrap" }} data-testid="cut">A very long value that does not fit its box</p>;
    }
    render(<Probe />);
    const cut = screen.getByTestId("cut");
    Object.defineProperty(cut, "scrollWidth", { configurable: true, value: 300 });
    Object.defineProperty(cut, "clientWidth", { configurable: true, value: 100 });

    // jsdom does no layout, so the browser's answers for a cut value are stood in for.
    vi.spyOn(window, "getComputedStyle").mockReturnValue({ textOverflow: "ellipsis", overflowX: "hidden", overflowY: "visible", getPropertyValue: () => "none" } as unknown as CSSStyleDeclaration);
    fireEvent.pointerOver(cut);

    await waitFor(() => expect(cut.getAttribute("title")).toBe("A very long value that does not fit its box"));
  });
});
