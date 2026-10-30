import { act, renderHook } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { SIDEBAR_COLLAPSED_KEY, useSidebarCollapsed } from "./use-sidebar-collapsed";

type Listener = () => void;

function laptopScreen(matches: boolean) {
  const listeners = new Set<Listener>();
  const list = {
    matches,
    addEventListener: (_: string, listener: Listener) => listeners.add(listener),
    removeEventListener: (_: string, listener: Listener) => listeners.delete(listener)
  };
  window.matchMedia = vi.fn().mockReturnValue(list) as unknown as typeof window.matchMedia;
  return {
    resize(next: boolean) {
      list.matches = next;
      listeners.forEach((listener) => listener());
    },
    listeners
  };
}

describe("whether the sidebar is the narrow icon rail", () => {
  const original = window.matchMedia;

  beforeEach(() => window.localStorage.clear());
  afterEach(() => {
    window.matchMedia = original;
    vi.restoreAllMocks();
  });

  it("is full width on a wide screen with no choice made", () => {
    laptopScreen(false);
    expect(renderHook(() => useSidebarCollapsed()).result.current.collapsed).toBe(false);
  });

  it("shrinks to icons by itself on a laptop-width screen, and follows the screen as it changes", () => {
    const screen = laptopScreen(true);
    const { result, unmount } = renderHook(() => useSidebarCollapsed());
    expect(result.current.collapsed).toBe(true);

    act(() => screen.resize(false));
    expect(result.current.collapsed).toBe(false);

    unmount();
    expect(screen.listeners.size).toBe(0);
  });

  it("keeps whatever the person chose over the screen, whether they chose open or closed", () => {
    laptopScreen(true);
    window.localStorage.setItem(SIDEBAR_COLLAPSED_KEY, "false");
    expect(renderHook(() => useSidebarCollapsed()).result.current.collapsed).toBe(false);

    laptopScreen(false);
    window.localStorage.setItem(SIDEBAR_COLLAPSED_KEY, "true");
    expect(renderHook(() => useSidebarCollapsed()).result.current.collapsed).toBe(true);
  });

  it("ignores a saved value it does not recognise", () => {
    laptopScreen(true);
    window.localStorage.setItem(SIDEBAR_COLLAPSED_KEY, "maybe");
    expect(renderHook(() => useSidebarCollapsed()).result.current.collapsed).toBe(true);
  });

  it("toggles and remembers the choice", () => {
    laptopScreen(false);
    const { result } = renderHook(() => useSidebarCollapsed());
    act(() => result.current.toggle());
    expect(result.current.collapsed).toBe(true);
    expect(window.localStorage.getItem(SIDEBAR_COLLAPSED_KEY)).toBe("true");
    act(() => result.current.toggle());
    expect(result.current.collapsed).toBe(false);
    expect(window.localStorage.getItem(SIDEBAR_COLLAPSED_KEY)).toBe("false");
  });

  it("still toggles for this visit when the browser will not keep the choice", () => {
    laptopScreen(false);
    vi.spyOn(Storage.prototype, "setItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    const { result } = renderHook(() => useSidebarCollapsed());
    act(() => result.current.toggle());
    expect(result.current.collapsed).toBe(true);
  });

  it("starts from the screen's own width when the browser will not give back a saved choice", () => {
    laptopScreen(true);
    vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
      throw new Error("blocked");
    });
    expect(renderHook(() => useSidebarCollapsed()).result.current.collapsed).toBe(true);
  });

  it("stays full width in a browser that cannot report the screen's width", () => {
    window.matchMedia = undefined as unknown as typeof window.matchMedia;
    const { result, unmount } = renderHook(() => useSidebarCollapsed());
    expect(result.current.collapsed).toBe(false);
    expect(() => unmount()).not.toThrow();
  });
});

describe("the sidebar on a server render", () => {
  it("renders full width, because a server cannot know the screen", async () => {
    const { createElement } = await import("react");
    const { renderToString } = await import("react-dom/server");
    function Probe() {
      return createElement("span", null, useSidebarCollapsed().collapsed ? "icons" : "full");
    }
    expect(renderToString(createElement(Probe))).toContain("full");
  });
});
