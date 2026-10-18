import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
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
  useAuth: () => ({ token: "t", user: { displayName: "Sam Example", username: "sam", avatarInitials: "SE" }, logout: vi.fn() }),
  authedFetch: (...args: unknown[]) => live.authedFetch(...args)
}));

vi.mock("../lib/use-attention", () => ({
  useAttention: () => ({
    failedJobs: 0, failedTitleJobs: 0, failedBackgroundJobs: 0, deadLetteredJobs: 0, indexerAlerts: 0, downloadsNeedingAction: 0,
    failedImportJobs: 0, movieWanted: 0, tvWanted: 0, healthProblems: 0, total: 0, summary: {}, loading: false,
    refresh: (...args: unknown[]) => live.attentionRefresh(...args)
  })
}));
vi.mock("../lib/use-host-name", () => ({ hostTitle: (name: string | null) => (name ? `Deluno · ${name}` : "Deluno"), useHostName: () => null }));
vi.mock("../lib/use-cut-value-titles", () => ({ useCutValueTitles: () => {} }));
vi.mock("../lib/route-prefetch", () => ({ prefetchMainRoutesWhenIdle: () => {}, prefetchHandlers: () => ({}) }));
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
  queryClient.clear();
});
afterEach(() => { vi.useRealTimers(); });

describe("the command palette key", () => {
  it("Ctrl+K opens the command palette, also while the cursor is in a text box", async () => {
    await mount("/movies");
    expect(screen.queryByRole("dialog")).toBeNull();

    fireEvent.keyDown(document.body, { key: "k", code: "KeyK", ctrlKey: true });
    expect(await screen.findByRole("dialog")).toBeTruthy();
  });

  it("Ctrl+K from inside a text field also opens it, instead of typing a k", async () => {
    await mount("/movies");
    const box = screen.getByLabelText("A text box");
    box.focus();

    fireEvent.keyDown(box, { key: "k", code: "KeyK", ctrlKey: true });

    expect(await screen.findByRole("dialog")).toBeTruthy();
    expect((box as HTMLInputElement).value).toBe("");
  });
});

describe("g then a letter", () => {
  const destinations: Array<[string, string]> = [
    ["o", "/"], ["m", "/movies"], ["t", "/tv"], ["q", "/queue"], ["i", "/indexers"], ["x", "/search-cycles"],
    ["a", "/activity"], ["c", "/calendar"], ["d", "/discover"], ["s", "/settings"], ["y", "/system"]
  ];

  for (const [letter, path] of destinations) {
    it(`g then ${letter} goes to ${path}`, async () => {
      await mount(path === "/activity" ? "/movies" : "/activity");
      press("g");
      press(letter);
      await waitFor(() => expect(where()).toBe(path));
    });
  }

  it("does nothing once more than a second has passed after the g", async () => {
    await mount("/activity");
    vi.useFakeTimers();
    press("g");
    await act(async () => { await vi.advanceTimersByTimeAsync(1100); });
    press("m");
    await act(async () => { await vi.advanceTimersByTimeAsync(200); });
    expect(where()).toBe("/activity");
  });

  it("is ignored while typing in a text box, so a g in a name does not jump away", async () => {
    await mount("/activity");
    const box = screen.getByLabelText("A text box");
    box.focus();
    press("g", {}, box);
    press("m", {}, box);
    await settle();
    expect(where()).toBe("/activity");
  });

  it("is ignored with a modifier key held, so Ctrl+G and Ctrl+M keep their browser meaning", async () => {
    await mount("/activity");
    press("g", { ctrlKey: true });
    press("m");
    await settle();
    expect(where()).toBe("/activity");
    press("g");
    press("m", { altKey: true });
    await settle();
    expect(where()).toBe("/activity");
  });
});

describe("the theme button", () => {
  it("switches the theme, says what pressing it again would do, and saves the choice to settings", async () => {
    await mount("/");
    fireEvent.click(screen.getByRole("button", { name: "Switch to dark mode" }));

    expect(live.theme.setTheme).toHaveBeenCalledWith("dark");
    await waitFor(() => expect(live.authedFetch).toHaveBeenCalled());
    const [url, init] = live.authedFetch.mock.calls[0];
    expect(url).toBe("/api/settings");
    expect(init.method).toBe("PATCH");
    expect(JSON.parse(init.body)).toEqual({ uiTheme: "dark" });
  });

  it("reads Switch to light mode while the theme is dark, and pressing it saves light", async () => {
    live.theme.resolvedTheme = "dark";
    await mount("/");
    fireEvent.click(screen.getByRole("button", { name: "Switch to light mode" }));

    expect(live.theme.setTheme).toHaveBeenCalledWith("light");
    await waitFor(() => expect(JSON.parse(live.authedFetch.mock.calls[0][1].body)).toEqual({ uiTheme: "light" }));
  });
});

describe("the phone search sheet", () => {
  it("the / key opens the search sheet below the large width (the library matches no width here)", async () => {
    await mount("/");
    fireEvent.keyDown(document.body, { key: "/", code: "Slash" });
    expect(await screen.findByRole("button", { name: "Done" })).toBeTruthy();
  });

  it("the search icon opens a full-screen sheet; Done closes it", async () => {
    await mount("/");
    expect(screen.queryByRole("button", { name: "Done" })).toBeNull();

    fireEvent.click(screen.getByRole("button", { name: "Search & navigate" }));
    expect(screen.getByRole("button", { name: "Done" })).toBeTruthy();
    expect(screen.getByRole("button", { name: "Open search & navigate" })).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Done" }));
    expect(screen.queryByRole("button", { name: "Done" })).toBeNull();
  });

  it("Open search & navigate closes the sheet and opens the command palette", async () => {
    await mount("/");
    fireEvent.click(screen.getByRole("button", { name: "Search & navigate" }));
    fireEvent.click(screen.getByRole("button", { name: "Open search & navigate" }));

    expect(screen.queryByRole("button", { name: "Done" })).toBeNull();
    expect(await screen.findByRole("dialog")).toBeTruthy();
  });

  it("its own text box is wired to nothing: typing there changes no screen and opens no results (only the button works)", async () => {
    await mount("/activity");
    fireEvent.click(screen.getByRole("button", { name: "Search & navigate" }));
    const box = screen.getByPlaceholderText("Search...");
    fireEvent.change(box, { target: { value: "alien" } });

    expect(where()).toBe("/activity");
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(screen.queryByText("alien", { selector: "li, [role=option]" })).toBeNull();
  });
});

describe("an import finishing anywhere", () => {
  it("raises 'is in your library' with an Open Transfers button that goes to Transfers, and the attention counts are read again", async () => {
    await mount("/movies");
    sendEvent("DispatchImportCompleted", { dispatchId: "d1", releaseName: "Alien.1979.1080p", succeeded: true });

    expect(live.toast.success).toHaveBeenCalledTimes(1);
    const [text, options] = live.toast.success.mock.calls[0];
    expect(text).toBe("Alien.1979.1080p is in your library.");
    expect(options.action.label).toBe("Open Transfers");
    expect(live.attentionRefresh).toHaveBeenCalledTimes(1);

    act(() => { options.action.onClick(); });
    await waitFor(() => expect(where()).toBe("/queue"));
  });

  it("raises 'Import failed for <release>. <reason>' with a See why button, and the attention counts are read again", async () => {
    await mount("/movies");
    sendEvent("DispatchImportCompleted", { dispatchId: "d2", releaseName: "Heat.1995.1080p", succeeded: false, failureReason: "The file is not a video." });

    expect(live.toast.error).toHaveBeenCalledTimes(1);
    const [text, options] = live.toast.error.mock.calls[0];
    expect(text).toBe("Import failed for Heat.1995.1080p. The file is not a video.");
    expect(options.action.label).toBe("See why");
    expect(live.toast.success).not.toHaveBeenCalled();
    expect(live.attentionRefresh).toHaveBeenCalledTimes(1);

    act(() => { options.action.onClick(); });
    await waitFor(() => expect(where()).toBe("/queue"));
  });

  it("a failure with no reason says only that the import failed, with no stray text after it", async () => {
    await mount("/movies");
    sendEvent("DispatchImportCompleted", { dispatchId: "d3", releaseName: "Heat.1995.1080p", succeeded: false });
    expect(live.toast.error.mock.calls[0][0]).toBe("Import failed for Heat.1995.1080p.");
  });
});

describe("after a reconnect that could not replay what was missed", () => {
  it("every query is invalidated once and the route's own data is read again once", async () => {
    await mount("/movies");
    const invalidate = vi.spyOn(queryClient, "invalidateQueries");
    const loadsBefore = loads;
    expect(live.resync).toBeTypeOf("function");

    await act(async () => { live.resync!(); });

    expect(invalidate).toHaveBeenCalledTimes(1);
    expect(invalidate).toHaveBeenCalledWith();
    await waitFor(() => expect(loads).toBe(loadsBefore + 1));
    invalidate.mockRestore();
  });
});
