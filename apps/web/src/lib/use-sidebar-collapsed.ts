import { useCallback, useState, useSyncExternalStore } from "react";

/** Where the choice is kept in this browser. */
export const SIDEBAR_COLLAPSED_KEY = "deluno.sidebar.collapsed";

// Just beside the 1024px where the sidebar appears, the menu shrinks to icons by itself so the page keeps its room,
// as Weir's does at its laptop widths. A click on Collapse or Expand overrides it, and is remembered.
const LAPTOP_WIDTH = "(min-width: 1024px) and (max-width: 1099px)";

function useMediaQuery(query: string): boolean {
  return useSyncExternalStore(
    (onChange) => {
      if (typeof window.matchMedia !== "function") return () => undefined;
      const list = window.matchMedia(query);
      list.addEventListener("change", onChange);
      return () => list.removeEventListener("change", onChange);
    },
    () => typeof window.matchMedia === "function" && window.matchMedia(query).matches,
    () => false
  );
}

function readChoice(): boolean | null {
  try {
    const stored = window.localStorage.getItem(SIDEBAR_COLLAPSED_KEY);
    return stored === "true" ? true : stored === "false" ? false : null;
  } catch {
    return null;
  }
}

/** Whether the sidebar is the narrow icon rail, and the switch for it. */
export function useSidebarCollapsed(): { collapsed: boolean; toggle: () => void } {
  const [choice, setChoice] = useState<boolean | null>(readChoice);
  const laptop = useMediaQuery(LAPTOP_WIDTH);
  const collapsed = choice ?? laptop;
  const toggle = useCallback(() => {
    const next = !collapsed;
    setChoice(next);
    try {
      window.localStorage.setItem(SIDEBAR_COLLAPSED_KEY, String(next));
    } catch {
      // Private window or blocked storage: the choice still holds until the page is closed.
    }
  }, [collapsed]);
  return { collapsed, toggle };
}
