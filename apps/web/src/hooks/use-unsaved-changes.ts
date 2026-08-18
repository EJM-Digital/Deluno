/**
 * Guard against losing edits: blocks in-app navigation (react-router data
 * router) and the browser unload while `dirty` is true.
 *
 * Returns the blocker so the caller can render a confirm and then
 * `blocker.proceed()` / `blocker.reset()`.
 */
import { useEffect } from "react";
import { useBlocker } from "react-router-dom";

export function useUnsavedChanges(dirty: boolean) {
  const blocker = useBlocker(({ currentLocation, nextLocation }) => dirty && currentLocation.pathname !== nextLocation.pathname);

  useEffect(() => {
    if (!dirty) return;
    const handler = (event: BeforeUnloadEvent) => {
      event.preventDefault();
      // Chrome requires returnValue to be set for the prompt to show.
      event.returnValue = "";
    };
    window.addEventListener("beforeunload", handler);
    return () => window.removeEventListener("beforeunload", handler);
  }, [dirty]);

  return blocker;
}
